# DB-057：Guid、decimal 与 TimeSpan 内建标量值槽

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0057-bcl-scalar-value-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：三值完整表示、双比较和两代包证据见 §9；ValueTuple 多 child 问题保在 §8，history v8 只属当片写入版本。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：已实施 / G0–G3，2026-09-10。完成证据与实际边界见 §9。
> 设计调查基线：`1c38768`（DB-056）；施工基线：`67f7209`。§1–§8 保留选片依据与采纳合同，§9 记录实际实现。
> 当前能力：[PROJECT-STATE](../../../src/PROJECT-STATE.md)；后继选择：[路线图](../../DurableGraph-research-roadmap.md)。

## 1. 本片回答什么问题

让普通领域模型直接使用 `Guid` 业务标识、`decimal` 精确数量和 `TimeSpan` 持续时间，
并在字段、泛型、Nullable、数组、List、Dictionary 中沿用已有冻结、差分、历史读取及显式升级。
应用无需为这三种固定语义值编写 Durable 包装类型，也无需将其改成 string 或普通整数。

最小成功标准：真实 PackageReference 消费者保存同时包含三种值及其组合的 World，
同实例修改后 Commit、冷重开精确恢复；第二代程序保留 history，显式升级旧 owner/inline DTO 后
强制 Base 续写，再次无修改为 NoChange、后续修改能产生正确 Delta。旧领域 inline 声明可以删除。

这里的“标量”指没有递归持久子槽的内建值，不意味着 CLR Primitive，也不意味着所有编码等长。

| 候选 | 价值与代价 | 本轮建议 |
|---|---|---|
| Guid + decimal + TimeSpan | 业务 ID、精确数量、持续时间；三者无引用、无子布局，复用一次 builtin/history/SG/包验收改造 | 一起实施，边界由这三种明确语义决定 |
| 仅 Guid | 有独立实用价值，但后两者会重复支付相同贯穿成本 | 没有仅需 Guid 的消费者约束，不人为拆三轮 |
| ValueTuple | 免声明的复合字段/Key；但要支持多个子槽各自的 exact 版本、Item/Rest 与历史反推 | record 已提供有名复合值替代，作为独立后继，见 §8 |
| 跨程序集 | 有利于较大模型分层 | 外部 history、helper 可见性、规则登记与独立演化共同变化，先有具体消费者 |
| SchemaStore 复用 StateStore | 长期可复用版本化集合 | Dictionary 可用并不自动选定联合视图、冲突作用域或内建元数据自举；当前没有净删除路径 |

独立选片审视起初偏向 Guid 单片，经公共改造成本比较后推荐三值合片；
另一路 Tuple 设计审视确认多 child 是真实结构变化，同意本轮先做叶子值。
这不是否决 Tuple，也不是以实现容易取代用户价值。

## 2. 实施前事实与需要贯通的接缝

调查基线只支持 13 种标量，string 是独立引用对象。下面是实施前的接缝盘点；新能力与执行见证见 §9：

- [TypeTag](../../../src/DurableGraph/Schema/TypeTag.cs) 的 1–14 包含已有标量/string；15、16、18 分别为引用、inline、Nullable，17 留给 history 参数。
  [TypeExpr.Builtin](../../../src/DurableGraph/Schema/TypeExpr.cs) 与 [共享 TypePattern](../../../src/Shared/SchemaHistoryTypePattern.cs) 把 builtin 限于 1–14。
- [BuiltinStateValues](../../../src/DurableGraph/Runtime/State/BuiltinStateValues.cs) 的 current/stored 绑定、`IdentityValueProjection<T>`、静态 `IStateOps<T>`
  已能承载无引用 unmanaged 值；这里没有这三种 CLR 类型的分支。
- [BinaryPayloadWriter](../../../src/DurableGraph.Serialization/BinaryPayloadWriter.cs) /
  [Reader](../../../src/DurableGraph.Serialization/BinaryPayloadReader.cs) 尚无这三种方法；有符号整数采用 canonical ZigZag varint。
- [Generator](../../../src/DurableGraph.Generator/DurableSchemaGenerator.cs) 的识别/类型名/tag，
  [普通 body](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GeneratedState.cs) 与
  [Family body](../../../src/DurableGraph.Generator/DurableSchemaGenerator.GenericState.cs) 都需要接入。
  仅新增 Runtime 绑定不能使 SG 接受字段，也不能保证两条生成路径的 decimal 相等性一致。
- [history 解析/模板](../../../src/DurableGraph.Generator/DurableSchemaGenerator.TemplateHistory.cs) 与
  [Build 工具](../../../src/DurableGraph.Build/SchemaHistoryTool.cs) 有连续 tag 上界和格式能力判定；
  新标量可能出现在直接字段，也可能只出现在 Nullable、容器、泛型/base 的 nominal 实参里。
- [StateModelSnapshot](../../../src/DurableGraph.Persistence/StateModelSnapshot.cs) 需要闭合 CLR ↔ nominal ↔ stored 槽双向映射；
  [TypeExpr wire](../../../src/DurableGraph.Persistence/TypeExprWireCodec.cs) 与
  [目录 codec](../../../src/DurableGraph.Persistence/SchemaCatalogWireCodec.cs) 的 ReadSlot 也各自只把 1–14 当叶子。
- [DictionaryKeyPolicy](../../../src/DurableGraph/Runtime/Containers/DictionaryKeyPolicy.cs) 的 current key 入口能复用 builtin binding，
  但 `TryScalarTag` / `ReadScalar` 仍须同步，否则会出现 current 接受、stored 拒绝。
  [StateBodySize](../../../src/DurableGraph/Runtime/State/StateBodySize.cs) 需补新叶子的最小字节数。

建议收敛明确的 builtin tag 判定，避免多个 `<= 14` 散落失配；不能改成 `<= 21` 而把中间复合 tag 当叶子。
只提取本片实际需要的固定判定/映射，不引入可注册标量插件、另一套 Schema 权威或通用 codec 平台。

## 3. 支持范围与状态表示

三者都是显式内建叶子，不要求 `[DurableType]`，不拥有用户 SchemaId/版本、ObjectId 或独立目录行。
DTO 直接保存 `Guid`、`decimal`、`TimeSpan` CLR 值；按值 Capture/Hydrate，沿现有 unmanaged 泛型约束组合。
它们的字段 tag 保持名义区别：TimeSpan 不是 Int64，Guid 不是两个 UInt64，decimal 不是用户 inline struct。

同片覆盖：

- 普通/readonly 字段，普通/generic struct 与 DB-056 record backing storage。
- 开放泛型参数闭合为上述值、phantom nominal 实参、Nullable 包装。
- SZ/rank 2–4 数组、List、Dictionary 的 key/value，以及上述形状的递归组合。
- 当前/历史 reader、owner 显式 Upgrade、已声明的值工具和容器元素/双槽规则。

已知字段直接静态调用 Writer/Reader；泛型未知槽使用现有静态约束操作。
不在成员循环新增 Type 哈希查找、装箱比较或 ValueSlotCodec callvirt。
decimal 相等性应有一处可供两种 SG body 与 Runtime 调用的强类型实现，不能三处各写一套位比较。

本片不扩展 DateTime/DateTimeOffset、DateOnly/TimeOnly、native int、Int128/UInt128、BigInteger、ValueTuple、
跨程序集、object/interface 通配槽、boxed value identity 或根 Nullable Key。
DateTime 的时间/隐藏状态合同另选；DateOnly/TimeOnly 并非已知技术障碍，只是本片以三类常见值封顶。
标准库类型按当前 Compilation 的真实符号/Runtime Type 识别，不把同名用户类型当作内建。

## 4. 精确状态与 Base / Delta 编码

实现采用下面的编码合同；不依赖私有 CLR 字段顺序、native memory dump、当前文化或文本解析。

| 类型 | 持久状态相等性 | Base 建议 | 读取校验 |
|---|---|---|---|
| Guid | 完整 Guid 值相等 | 固定 16 byte，明确 `bigEndian: true` | 必须有完整 16 byte；不额外限制 GUID version/variant 或 Empty |
| decimal | `GetBits` 的 lo/mid/hi/flags 全部相同 | 四个 32-bit word，按 lo、mid、hi、flags 顺序，各固定 little-endian，共 16 byte | flags 保留位必须为零、scale 0–28；拒绝截断/非法表示 |
| TimeSpan | 有符号 Ticks 相同 | 复用 `WriteInt64(value.Ticks)` 的 ZigZag varint，1–10 byte | 复用 canonical Int64 截断、溢长和溢出校验 |

Guid 的 API 支持显式端序，使用对应的 span 构造读取；独立 golden 应固定
`00112233-4455-6677-8899-aabbccddeeff` 对应 `00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF`。
不混用省略端序参数的重载。依据：[Guid.TryWriteBytes](https://learn.microsoft.com/en-us/dotnet/api/system.guid.trywritebytes?view=net-10.0)、
[Guid 构造器](https://learn.microsoft.com/en-us/dotnet/api/system.guid.-ctor?view=net-10.0)。

decimal 保存 scale 和符号，包括带符号零：`1.0m → 1.00m` 是真实持久变化，不能用 `==` 消除。
使用公开 `GetBits` 的 span 重载与公开构造，避免临时数组、格式化或算术归一化。
“canonical”在这里表示每个合法完整表示有唯一编码，不把数值相等的不同表示合并。
例如 `1.0m` 的四字为 `[10, 0, 0, 0x00010000]`；测试应独立给字节并核对读后四字。
公开表示、scale/保留位及正负零语义依据：[Decimal.GetBits](https://learn.microsoft.com/en-us/dotnet/api/system.decimal.getbits?view=net-10.0)。

TimeSpan 只保存 Ticks；零、负值、MinValue/MaxValue 全部有效，不保存 TotalSeconds 等浮点投影。
数值单位由 TimeSpan 内建合同决定，不执行时区转换。依据：[TimeSpan.Ticks](https://learn.microsoft.com/en-us/dotnet/api/system.timespan.ticks?view=net-10.0)。

三者 Delta 均复用现有标量整值替换：相等为 `HasChanges=false`；不同则写一次新值 Base bytes。
Apply 必须拒绝宣称变化却与 prior 持久相等的 payload；嵌套 reader 不吞后续字段，完整 body 入口仍检查尾随字节。
不增加数值差量算法或 matcher 配置。MinimumBaseBytes 分别为 16、16、1。
准备和 B/D/H 计量继续消费实际 body；所有上层对象和发布规则不变。

## 5. 类型、history 与字典合同

### 5.1 tag 与格式边界

新增 `TypeTag.Guid=19`、`Decimal=20`、`TimeSpan=21`；沿用 `TypeExprKind.Builtin`，无新增 TypeExpr 构造。
共享 history 的 nominal 叶子使用 `b19`、`b20`、`b21`，FieldTag 与实际 builtin kind 匹配。
参数 tag 17、Nullable 18 与新叶子必须明确区分；未知 tag、错误 tag/operand 组合继续拒绝。

| 层 | 建议 | 原因 |
|---|---|---|
| 构建 manifest / `.dgschema` | 新写格式 v8，v1–7 继续按原能力读取 | 新叶子需明确版本门槛，保留既有代码历史；旧文件名/hash/内容不重写 |
| 统一 Schema 目录 SCB1 | 保持 v2，新增三个未使用的叶子 tag | 目录记录/依赖结构没有变化；既有 tag 字节解释不变，旧程序对未知 tag 明确拒绝 |
| 对象 Base / Storage / List / Dictionary | 保持 Base v4、Storage v3、List codec 2、Dictionary codec 1 | 只扩已带类型的叶子内容，不改变对象 envelope 或容器编辑 grammar |

v1–7 的限制也要递归应用到所有 TypePattern：不能让 `List<Guid>`、`Phantom<decimal>` 或 base 实参
绕过格式门槛。包括较早的独立非模板 history reader，不得因共享 TryGetFieldTypeName 扩展而误接收新 tag。
当前版本工具仍应 Publish/Verify 已接受的旧 history，不能因生成 manifest v8 强迫无布局变化的定义升版。
不得重新引入此前已移除的 State/Schema 旧 wire 只读兼容或双写路径。

SCB1 v2 增加 leaf code 是本片推荐，G0 必须以独立目录 bytes 与旧 tag 不变见证检查；
若发现既有合法编码被重解释，才改目录版本并明确拒绝旧格式，不通过宽松 reader 混过。
Runtime/Build/SG 的格式判断不能仅由某一层接受就算完成。

### 5.2 Schema 与升级

这三种叶子的语义由 framework codec 确定，无需用户升一个 BCL SchemaVersion。
用户字段 `long → TimeSpan` 或 `double → decimal` 仍是 owner 布局变化，须显式升版和业务 Upgrade，
框架不猜单位、精度或舍入；inline/base 传播、引用边截断规则保持。

静态类型既不等于持久字节相同，也不提供自动转换授权。
Runtime `StateValueUpgradeProvider` 已能表达 builtin 端点，本片只贯通三个新叶子的绑定；
沿用已有 KeepExact、显式规则与 Nullable lifting 次序，不新增 Upgrade 属性/转换注册平台。
容器规则仍由 owner 显式选择；空容器也预检完整双端/工具，不因三者无引用而放松原合同。
这里的容器升级保持 nominal 类型，例如 `List<Price>` 内 Price 的 Schema 升版。
本片不授予 `List<long> → List<TimeSpan>` 或 Dictionary 更换 key/value nominal 类型的迁移能力；
这与 owner 的直接值字段显式 `long → TimeSpan` 转换不同，也不放开创建新持久对象。

### 5.3 Dictionary 的两种相等性

新内建 key 的默认比较使用已有 ScalarDefault 模式；Application 仍可明确选择当前规则。
`TryScalarTag`、`ReadScalar`、stored 标准 lookup 校验与 current 恢复必须成组扩展。
这不把包含它们的普通/record struct 自动改为 ScalarDefault；复合 Key 继续沿 DB-055。

- Guid/TimeSpan 的完整表示与默认值相等性一致。
- decimal 的数值相等性与持久表示相等性分开：真正更换 key 存储，从 `1.0m` 到 `1.00m`，
  Delta 使用 Remove+Add；其 value 没变也不能把这次 key 表示变化吞掉。
- BCL 默认 Dictionary 的 indexer 给相等 key 赋 value 不保证替换原 key；测试需 Remove 后 Add，
  再从实际捕获结果证明 key 表示改变，不能仅凭调用语句推断。
- 标准模式的损坏 DTO 同时含数值相等、canonical bytes 不同的 decimal key，仍拒绝 lookup 冲突；
  Application 模式继续只在 current TryAdd 检查相应业务规则，不提前执行它。
- 不改 comparer 标签、回捕稳定性或“失败不交付部分 World”的恢复合同。

## 6. 施工顺序与最小验收

| 阶段 | 工作与依赖 | 验收证据 |
|---|---|---|
| G0 固定合同 | 核对 tag 表、CLR unmanaged、公开 span API、两个生成路径和 history 门槛；先做最小 codec/编译见证 | 三值可直接作为 frozen state；Guid 端序、decimal 四字与 TimeSpan 极值往返；没有旧 tag 重解释 |
| G1 Runtime / Serialization | 三值 Writer/Reader、唯一 decimal 状态比较、静态 ops 与 current/stored 映射、最低字节数、字典标准 key | 独立 Base/Delta golden；相等性和 Delta 一致；非法 decimal、截断、未知码、尾随及伪变化拒绝 |
| G2 Schema / Build / SG | 明确叶子分类、history v8 和既有文件保留、目录 leaf、普通/Family body、类型/模板映射 | 直接及递归组合生成并执行；固定 v7 history 与 v8 manifest 同布局 Publish/Verify 保持旧文件名/hash/bytes；旧格式里嵌入新 tag 拒绝；同版改字段诊断 |
| G3 产品闭环 | 连续会话、字典表示替换、显式迁移、真实包两代、独立审查与文档 | exact decode → Upgrade → 原图恢复 → 强制 Base → NoChange / Delta → 冷重开；无隐式迁移或基线漂移 |

独立断言至少包含：

1. Guid Empty/任意 128 bit、固定端序 golden；decimal 多 scale、不同 scale 的同数值、正负零、极值、scale 29/保留位非法；
   TimeSpan 0、±1 tick、MinValue/MaxValue 与非 canonical varint。
2. 原候选不受后续字段/容器修改污染；同版只改变 decimal scale 时，直接字段、Nullable、inline、List value 的持久差分均可见。
3. `Dictionary<decimal, V>` 的真实 key 替换为 Remove+Add；相同 key 只改 value 使用 PatchValue；
   标准数值冲突拒绝、Application 合法不同选择继续工作。Guid/TimeSpan key 默认模式 Load→Capture 稳定。
4. 用新 tag 占据泛型 phantom/base/Nullable/container 的不同位置，证明 nominal 和格式验证并未只覆盖直接字段。
   SG 不接受伪 BCL 类型；原未知值、boxed、跨程序集和 Nullable 根 key 负例保留。
   至少一个纯非 Family 编译直接使用三种新叶子，另一编译通过泛型/Nullable 触发 Family；不新增“有新叶子就切 Family”的规则。
5. 两代真实包包含带三值的旧 inline DTO，第二代删除旧 CLR、显式升级 owner/inline，
   同时覆盖无 inline 升级时的三值历史 exact 读取。标准规则与新增 builtin 的组合不能依赖 ProjectReference 偶然可见性。

两代新消费者都可能由新工具生成 history v8，不能拿它们替代 G2 的旧 v7 保留见证。
固定 v7 fixture 只能含当时支持的叶子；另造旧 header 下直接/嵌套新 tag 的非法样本，以验证版本能力门槛。

实施后运行根 `dotnet build DurableGraph.slnx`、相关定向 tests 及根 tests；真实 PackageReference 先跑新增三值 lane，
再根据公共 history/SG 改动跑已有 Generic、Nullable、CompositeDictionary、Record lane。
所有 dotnet/build/package 验证由主线程串行调度；现有测试需要更新断言，不整体删除含其他不支持类型的旧用例。
完成证据集中记入本分片；PROJECT-STATE 只更新能力和后继，target 仅提升最终采纳的持久语义。

建议分派：G0 主线程持有合同；之后 Serialization/Runtime ops、SG/history、StateStore/Dictionary 集成测试
按互斥文件分工。共享 tag/history 接口先落定再并行，独立 reviewer 检查 decimal 相等性、格式门槛、字典两个比较层。
不以子任务摘要代替主线程集成 diff、测试、包消费与文档链接验收。

## 7. 设计选择与回退条件

本片选择保留 decimal 完整公开表示，而非仅保存数值；这延续浮点 bits / Dictionary canonical key 的已有区分。
Guid 的端序、decimal 固定 16 byte、TimeSpan varint 是简单明确的初期编码，不宣称最小尺寸或最佳性能。
未知 generic 槽继续静态 IStateOps，已知字段静态绑定；不为三种叶子引入通用平台或反射成员遍历。

若实施发现某种类型无法在公开稳定 API 下精确往返，或必须改变既有历史解释/自动业务转换，
应先报告具体反例并重新裁决；不能悄悄丢弃状态后仍称完整支持。
局部实现名、文件拆分和测试组织可以因地制宜；不得用只支持直接字段代替完整组合的验收。

## 8. ValueTuple 后继保留的问题

本轮调查确认 ValueTuple 可以沿内建值路线继续，并非 SG 无法实现；成本集中在多 child 历史表示。
详细后继合同留待选片，以下是需要保留的反例和依赖，不是本片施工清单：

- `(Point, Color)` 的一个字段可能同时需要 Point v1 与 Color v2；当前 `DurableFieldInfo.ValueSchema`、
  `StateFieldTemplate` / `StateParameterTemplate` 的单个 InlineVersion 不能表达这件事。
  应比较递归值模板/多 child 完整槽的最小替换；不能只取第一个 child，也不能把所有 child 版本合成一个假的用户 SchemaVersion。
- 显式 tuple 声明中的固定 Point/Color，与字段 `T` 恰好闭合到同 tuple，具有不同参数来源；
  沿用 DB-052 的来源区分，模板匹配、DTO 反推及 Upgrade selection 必须一致。
- 引用 Item 仍只保存 nominal + ObjectId，目标自己的 Base 决定其版本；不能因 tuple 泛型参数而追进目标 exact 布局。
- 明确 `ValueTuple` 0 元、1–7 元与 `TRest` 的支持、嵌套和 C# tuple 名称处理；
  要让既有 depth/node 界限覆盖完整结构，并以 Roslyn 编译见证验证语法/显式类型映射。
- 所有 child 的闭包登记/缓存复核和显式 Upgrade 必须同片贯通，包括空容器；
  不用伪用户 DefinitionId 或仅保存当前值、把历史读取留 TODO 的捷径。

record 已提供有名复合值；未来有 tuple 建模需求时，本节是技术调查入口，不能从已支持 record 推导 tuple 已支持。

## 9. 实施合同与验收账本

施工基线 `67f7209`，工作区干净；本轮只实施 §3–§7，§8 的 Tuple 后继不进入代码。
冻结 tag 19/20/21、Guid big-endian、decimal 四字 little-endian、TimeSpan canonical Int64；history 新写 v8，其他格式沿 §5.1。
Serialization 提供三种 Read/Write 及唯一 `ScalarStateEquality.DecimalEquals(in decimal, in decimal)`；
Runtime/SG 使用该比较，未知泛型槽仍走静态操作。不改变会话、发布、容器 nominal 升级或业务转换权限。

| 要求 | 实现落点 | 验收证据 |
|---|---|---|
| G0 / G1 字节与完整表示比较 | Serialization Reader/Writer、唯一 ScalarStateEquality | [BclScalarPayloadTests](../../../tests/DurableGraph.Serialization.Tests/Serialization/BclScalarPayloadTests.cs)：独立 golden、全部 scale/符号、非法 flags、极值/截断、unmanaged |
| G1 Runtime/current/stored/目录/字典 | TypeTagFacts、BclScalarStateValues、StateModelSnapshot、目录与 DictionaryKeyPolicy | [静态操作/最低尺寸](../../../tests/DurableGraph.Tests/BclScalarStateTests.cs)、[独立目录 bytes](../../../tests/DurableGraph.Persistence.Tests/BclScalarCatalogTests.cs)；新叶子不创建 Schema 行，旧 tag bytes 不变 |
| G2 SG 普通/Family/组合 | 真正 corelib 符号识别、普通 body 资格、唯一 decimal 比较与静态读写 | [BclScalarGeneratorTests](../../../tests/DurableGraph.Tests/BclScalarGeneratorTests.cs)：分离 compilation、实际执行 golden、nominal/base/phantom/容器、伪类型拒绝 |
| G2 history v8 / 旧文件 | Shared TypePattern、Build、SG history reader | [BclScalarHistoryTests](../../../tests/DurableGraph.Tests/BclScalarHistoryTests.cs)：固定 v7 filename/hash/bytes 保留，v1–7 直接/递归拒绝，旧 CLR 删除后的 exact reader |
| G3 差分与连续图 | 既有 GraphSession、容器和规则，不新增对象机制 | [14 次 Commit 图](../../../tests/DurableGraph.Tests/BclScalarGraphTests.cs)：decimal scale 分形状修改，键 Remove+Add / value Patch；[显式值工具](../../../tests/DurableGraph.Tests/NullableUpgradeTests.cs)：long→TimeSpan 与 Nullable lifting，缺规则零 callback |
| G3 真实包历史 | [BclScalarConsumer](../../../experiments/PackageConsumerProbe/BclScalarConsumer/README.md) | 两代全部 marker 通过；history 3→5，删除 LegacyPoint、显式 owner/共享 List 升级、仅两对象强制 Base、NoChange→Delta、冷重开不重复升级 |
| 完整集成 | 主线程串行验证、独立只读审查 | 根 build 0 warnings/errors；2204 tests 全通过（Runtime/SG 1313、StateStore 610、Serialization 126、Storage 155），无 skip；最终审查无阻断 |

基线根 build 0 warnings/errors，原 2146 tests 全通过。最终根测试包括新增的 64 个展开用例；
六条已失效的 decimal 拒绝数据移出，另一些诊断/容器拒绝测试改用 DateTime/DateTimeOffset 保留原目的，未批量删除负例。

实际接口与格式沿 §3–§5：三种 CLR 值直接作 DTO，普通和 Family 均静态调用；
TypeTag 19/20/21、history v8、SCB1 v2、Base v4、Storage v3、List 2、Dictionary 1。
明确分开保留构建 history 与兼容旧 State wire；没有重新添加旧 State/Schema wire 只读分支。

施工校准：普通生成路径的 body 资格原有独立 `<=16` 上界，现与显式叶子分类同步；
固定 inline 字段生成的 World.V1/V2 是非泛型 DTO，真实包按实际形状引用，不能仅凭包含 inline 值就假设 DTO 有类型参数。
这些修正不改变既有版本传播或容器 nominal 升级边界。

执行日志保存在忽略的 `obj/db057-*`；可重跑命令为 §6 与新消费者 README。
BCL 新 lane 使用版本 `0.0.0-bcl-scalar-e2e.20260910062306.13584` 的隔离八包 feed；
成功产物位于 `experiments/PackageConsumerProbe/obj/bcl-scalar-20260910062842-27696-b1642476`。
同 feed 的 Generic（四阶段）、Nullable、CompositeDictionary、Record（各两代）全部 marker 通过。
既有活跃 runner 的新写 history 断言同步 v8，固定旧 fixture 保持原版本；没有以批量升级旧文件让测试通过。
提交前检查 13 份修改 Markdown 的 494 个本地链接与 45 个锚点，零错误；`git diff --cached --check` 通过。
