# DB-058：DateOnly / TimeOnly / DateTimeOffset 内建值槽

> 文档整理：2026-10-14；来源为 Git `8ef9fca` 中的 `docs/design-branches/0058-temporal-scalar-value-slice.md`，正文保留当片合同与历史验收含义。
> 阅读范围：三值完整表示、offset 持久相等性与两代包证据见 §9；不包含 DateTime Local/DST，history v9 只属当片写入版本。
> 当前能力与施工焦点见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)；旧稿中的“下一步”不自动成为当前工作单。

> 状态：**已实施 / G0–G3 验收通过**，2026-09-10。实施与验收账本见 §9。
> 调查基线：`0cd838f`（DB-057 已实施）。当前能力见 [PROJECT-STATE](../../../src/PROJECT-STATE.md)，
> 长期约束见[目标设计](../../DurableGraph-target-design-v0.md)，其他后继见[路线图](../../DurableGraph-research-roadmap.md)。

## 1. 问题与推荐

现有图 Capture、同实例 Commit、exact 历史读取、显式升级和容器已经闭合。
推荐下一片补齐 `DateOnly`、`TimeOnly`、`DateTimeOffset`，让普通领域模型直接保存日期、
日内时刻和带偏移时间戳，复用 DB-057 的内建叶子路径。此片不增加对象种类或多 child 布局。

**最小完整成功标准**：三值在字段、泛型、Nullable、数组、List、Dictionary 中按完整公开表示
保存并冷读；同一瞬间只改 offset 能产生真实 Delta；保留的旧 DTO 在删除旧领域 inline CLR 后
仍可读取并显式升级，升级续写 Base 后再次 Commit 为 NoChange。

| 候选 | 直接收益 | 额外成本及本次排序 |
|---|---|---|
| 三种日期时间值 | 常见业务日期、日内时刻与 Timestamp 字段/复合 Key 不必手工拆整数 | 已有无 child 的 builtin 接缝；DateTimeOffset 的两种相等性可复用 decimal 的分层。推荐本片 |
| ValueTuple | 方便匿名复合值、复合 Key | 一个槽可能同时依赖多个 inline 历史版本；模板、DTO 参数来源、依赖闭包和升级都须扩展。record 已提供有名替代，保留独立后继 |
| 跨程序集领域模型 | 拆分共享模型包和应用 | 需要具体跨包 consumer 决定 helper 可见性、history/升级能力归属；当前尚无足以确定边界的新需求 |
| SchemaStore 复用 StateStore | 复用元数据集合的版本管理 | Dictionary 使候选更接近，但内建元数据表示、联合发布与冲突作用域仍需单独设计；不作为三值前置 |

这不是继续穷举 CLR 类型的长期排期。三值完成后，按具体领域模型或工作流重新选片，
不自动接着做 native int、BigInteger、其他集合或通用 codec 注册平台。

## 2. 实施前事实与代码接缝

- [TypeTag](../../../src/DurableGraph/Schema/TypeTag.cs) 的 19/20/21 是 Guid/decimal/TimeSpan，
  `TypeTagFacts.IsBuiltin` 区分叶子与 15/16/17/18 的引用、inline、history parameter、Nullable。
  新三值目前不在支持范围；不能从 DTO 可装入一个 CLR struct 推导已经可保存。
- [BclScalarStateValues](../../../src/DurableGraph/Runtime/State/BclScalarStateValues.cs) 是静态 `IStateOps<T>` 例子；
  [BuiltinStateValues](../../../src/DurableGraph/Runtime/State/BuiltinStateValues.cs) 负责 current/stored 值绑定。
  生成器的普通 body 与 Family body 均需覆盖，不能仅在 Runtime 添加映射。
- [DictionaryKeyPolicy](../../../src/DurableGraph/Runtime/Containers/DictionaryKeyPolicy.cs) 已把标准领域查找与完整 key bytes 配对分开；
  [DB-057](../0057-bcl-scalar-value-slice/README.md) 已验证数值相等但 decimal 表示不同的键替换。
- [Shared TypePattern](../../../src/Shared/SchemaHistoryTypePattern.cs)、[Build history](../../../src/DurableGraph.Build/SchemaHistoryTool.cs)
  与 [SG history](../../../src/DurableGraph.Generator/DurableGraphGenerator.TemplateHistory.cs) 当前新写 v8，旧版本有递归能力门槛。
- ValueTuple 接缝不是序列化循环本身：[DurableFieldInfo.ValueSchema](../../../src/DurableGraph/Schema/DurableFieldInfo.cs)
  目前只提取一个 inline 依赖；[StateFieldTemplate/StateParameterTemplate](../../../src/DurableGraph/Runtime/Binding/StateDefinitionBinding.cs)
  只带一个 `InlineVersion`。具体多 child 反例保留在 [DB-057 §8](../0057-bcl-scalar-value-slice/README.md#8-valuetuple-后继保留的问题)。

## 3. 已采纳支持合同

### 3.1 范围与表示

仅识别实际 corelib 的三种类型，不凭 `System` 命名空间或简单名字授予内建身份。
三者直接作为 unmanaged DTO 值，新增 builtin leaf tag 为 **22 DateOnly、23 TimeOnly、24 DateTimeOffset**。
`TypeExprKind` 不新增构造；不创建用户 Schema、独立 ObjectId 或元数据对象行。

支持直接/readonly 字段、普通与 record inline struct、开放泛型字段/实参、phantom/base 实参、
Nullable、SZ/rank 2–4 数组、List 及 Dictionary key/value 的现有合法递归组合。
Dictionary 根 Nullable key、数组协变、boxed value、object/interface 通配字段等原限制保持。

### 3.2 公开状态与 wire

所有整数沿用现有 canonical varint；不转字符串、不依赖区域设置或主机本地时区，不读 CLR 私有布局。

| 类型 | 持久状态与编码 | 解码验证 | 持久相等性 |
|---|---|---|---|
| DateOnly | `DayNumber`，UInt32 varint，1–4 bytes | `0..3,652,058`，用 `DateOnly.FromDayNumber` 构造 | DayNumber 相同 |
| TimeOnly | `Ticks`，UInt64 varint，1–6 bytes | `0 <= ticks < TimeSpan.TicksPerDay`，用 ticks 构造器 | Ticks 相同 |
| DateTimeOffset | clock `Ticks` 的 UInt64 varint，随后 offset 的整分钟数 Int32 ZigZag；合计 2–11 bytes | offset 在 `[-840,840]`；clock 和扣除 offset 后的 UTC ticks 均在 DateTime 合法范围；用 `(long ticks, TimeSpan offset)` 构造器 | `EqualsExact`，即同时保留时刻和 offset |

DateTimeOffset 选择 clock ticks 是为了直接对应公开构造器；UTC ticks 加 offset 也可无损，
但本片只选一份规范形式，不在 body 里并存两种编码。不保存时区 ID、DST 规则或某个系统日历对象。
offset 只是固定 UTC 偏移，不能替代时区身份。

例如 `2026-09-10 08:00 +08:00` 与 `2026-09-10 00:00 +00:00` 的默认业务比较相等，
但公开表示不同，必须使 StateEquals=false、PrepareDelta 有变化，并精确恢复各自 offset。
已知类型可直接发出 `EqualsExact`；普通/Family/Runtime 保持同一语义，不必再造一套动态比较目录。

Writer 从公开有效 CLR 值取数。Reader 用局部候选 cursor 完成 varint、范围和构造校验后才提交 cursor；
非法范围、非规范 varint、截断拒绝。字段 reader 不擅自消费尾部；独立 body/applier 仍要求完整消费。
`StateBodySize.MinimumBaseBytes` 分别为 **1/1/2**，Nullable 仍为至少 1 byte。

Delta 复用标量完整替换模式，不引入“相差多少 ticks”的算术编码。
无变化返回 false/empty；Apply 收到表示完全相同的伪变化须拒绝。存在合法嵌套尾部时不能当成整个 payload 的尾随错误。

### 3.3 为什么 DateTime 留在另一个问题中

DateTime 不是实现不了，但它有本片三值没有的合同选择。官方 `ToBinary/FromBinary` 的 Local 处理
可能随接收机器时区或 DST invalid-time 规则调整结果；runtime 还有公开 Kind 不能单独表达的
ambiguous-DST 状态。因此不能把 `ToBinary` 当成无环境依赖的完整快照，也不能未作说明便丢掉该状态。

候选是只支持 UTC/Unspecified 并拒绝 Local、明确采用 ticks+公开 Kind 的规范化合同，
或选择可以保留更完整信息的专门表示。三者有不同用户可观察行为，本片不替用户作这项选择。
DateTime 全类型继续拒绝；也不自动把它转成 DateTimeOffset。
将来确有 DateTime 模型时再用 DST 重叠/跳跃和跨时区见证裁决，不要求本片启动这一实验。

技术依据（.NET 10 文档及固定 v10.0.0 源码；属于平台事实，以上编码为本项目已采纳合同）：

- [DateOnly.DayNumber](https://learn.microsoft.com/en-us/dotnet/api/system.dateonly.daynumber?view=net-10.0)、
  [DateOnly 源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/DateOnly.cs)。
- [TimeOnly.Ticks](https://learn.microsoft.com/en-us/dotnet/api/system.timeonly.ticks?view=net-10.0)、
  [TimeOnly 源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/TimeOnly.cs)。
- [DateTimeOffset.EqualsExact](https://learn.microsoft.com/en-us/dotnet/api/system.datetimeoffset.equalsexact?view=net-10.0)、
  [构造器、比较与范围验证源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/DateTimeOffset.cs)。
- [DateTime.ToBinary 的 Local adjustment 合同](https://learn.microsoft.com/en-us/dotnet/api/system.datetime.tobinary?view=net-10.0)、
  [DateTime 状态与转换源码](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/DateTime.cs)。

## 4. Dictionary 与升级

三值默认 comparer 复用 `ScalarDefault`；标准查找相等性用 CLR Default，持久匹配用完整 canonical key bytes。
DateTimeOffset 的默认查找只看同一瞬间，不会因为库保存 offset 就改写业务 Equals。

- 相同 key 表示，只改 value：复用 PatchValue。
- 真正 Remove 旧 key、Add 一个同瞬间不同 offset 的新 key：持久差分为 Remove+Add。
- 仅用等价 key 调用 indexer 不一定替换 BCL 字典内原 key；测试必须明确区分它与真实键替换。
- 标准模式中的两个同瞬间 key 即使 bytes 不同，也须按既有标准 lookup 校验拒绝。
  Application 可用包含 offset 的当前 comparer 接纳两者，沿既有登记及恢复冲突边界，不引入新模式。
- 作为复合 key 的 Timestamp 即使被领域 comparer 忽略，也继续完整保存。Load→Capture 保持 comparer 模式。

三种 builtin 没有自有业务 SchemaVersion；改变 owner 字段类型仍须显式升版并提供转换。
例如 long ticks→DateTimeOffset 的 offset 如何选择，是用户 Upgrade 函数的业务决定。
现有 Runtime builtin 值规则、Nullable lifting 和容器 owner 规则可组合，但不增加自动转换、路径搜索或新 ID 分配。
容器 nominal 改型（例如 `List<long>`→`List<DateTimeOffset>`）仍不在本片内。

## 5. 格式、history 与生成代码

- 新 manifest/新 `.dgschema` 使用 **v9**；已接受的 v1–v8 文件保留 filename/hash/bytes，不能重写旧文件伪升版。
  相同声明用新工具 Publish/Verify 不新增无变化 history；固定 v8 与新 v9 的见证不可由两代全 v9 包代替。
- v1–v8 拒绝新 tag 22–24，包括 nominal-only 的 phantom/base 参数及 List/Dictionary/Nullable 内部位置。
  v9 仍拒绝未知 tag 和错误表达式。19–21 依旧要求至少 v8，不能扩大旧 `ContainsBclScalars` 范围而让 v8 误接纳新类型。
- SCB1 v2 的 tagged leaf、Base v4、Storage v3、List codec 2、Dictionary codec 1 的结构不变；
  扩充叶子集合，不增加旧 State wire 兼容分支；不把 build history 保留和旧数据格式兼容混为一谈。
- 普通非 Family 编译继续普通 body；泛型/Nullable/record 等既有条件触发 Family。新三值本身不是切换生成路径的理由。
  已知字段直接静态调用 Reader/Writer/比较，未知泛型继续 IStateOps/IValueProjection；不增加热路径 Type 查表。
- 需要同步 builtin 分类、current/stored/nominal 映射、SG 类型识别及两个 body、目录 leaf、Dictionary scalar reader、最低尺寸。
  源码里以旧最大 tag 或连续范围判断的路径须逐一核对；旧“不支持 DateTimeOffset/DateOnly/TimeOnly”负例改用仍不支持的见证，保留其原测试目的。

Shared history 已有多个 `allow*` 参数。实施可在局部净减少重复时收敛为格式版本驱动的门槛，
但不以此为本片前置，也不建立通用能力注册平台；无论实现形状如何，既有格式的接受集合必须保持。

## 6. 施工顺序与验收

| 阶段 | 内容 | 最小可观察结果 |
|---|---|---|
| G0 冻结合同 | 核对三值公开 API、unmanaged、tag/格式版本、两类比较与不支持边界 | 独立 golden、DTO CLR 形状与有效范围确定；不依赖本地时区 |
| G1 字节与 Runtime | Reader/Writer、静态 ops、builtin 绑定、最低尺寸及 Dictionary 标准 key | 三值 Base/Delta 往返；完整表示相等；非法/截断/伪变化拒绝；先验证候选 cursor |
| G2 SG/history/目录 | 两生成路径、递归组合、v9 新写与旧格式门槛、目录 leaf | 直接与泛型真实执行；同版改字段拒绝；固定 v8 字节不变；新 tag 递归旧格式拒绝 |
| G3 产品与包 | 同实例 Commit、key 替换、历史 reader、显式升级与真实包两代 | 冷读完整表示；历史 exact 零业务回调；删除旧 inline CLR 仍升级；强制 Base→NoChange→普通 Delta |

关键回归包括：

1. DateOnly Min/Max、闰日；TimeOnly 午夜、最后一个 tick；DateTimeOffset ±14h、0 offset、
   最小/最大合法 clock/UTC 边界、跨日且同瞬间不同 offset。正、负 offset 各有独立 golden，
   固定 clock ticks 在前、整分钟 Int32 ZigZag 在后的次序和单位，不只做读写互验。
2. 非法 DateOnly DayNumber、TimeOnly 24h、offset ±841、合法 clock/offset 但 UTC 越界、UInt64 大于 long.MaxValue、
   `clock=DateTime.MaxValue.Ticks+1`、非 canonical/截断 varint；失败 cursor 不推进。
   clock=0 配 +14h、clock=Max 配 -14h 是 UTC 越界见证；clock 上界和 Int64 上界分别验证。
3. DateTimeOffset 直接、Nullable、inline/record/generic、数组/List 的表示变化均可见；
   frozen prior 不受后续领域修改污染；同表示的伪变化 applier 拒绝。
4. 三值标准字典 Key/Value、DateTimeOffset key 的真实 Remove+Add 与 value Patch、标准同瞬间碰撞、
   Application 含 offset comparer 的双条目、复合 Key 忽略 Timestamp 的业务规则与完整保存。
5. 分离普通/Family compilation；正例含 phantom/base、SZ/rank 2–4 与容器递归；
   source-defined `System.DateOnly` 等 lookalike 拒绝。DateTime、boxed、未支持值、Nullable 根 key 负例仍有效。
6. 固定 v8 history+v9 manifest 同布局 Publish/Verify 无新文件且旧文件原样；
   所有旧格式直接/递归新 tag 拒绝；历史 DTO 包含三值且不依赖旧 inline CLR 声明。
   若收敛 history 门槛，另回归各旧格式原本支持的数组/List/Nullable/Dictionary/19–21 组合，避免误收紧旧接受集合。

真实包建议新增 `TemporalScalarConsumer`，复用既有 runner 的隔离 feed/独立进程方式，不使用 ProjectReference
或手工接 Analyzer/AdditionalFiles。第一代包含三值及容器，制造 offset 表示变化/字典真实 key 替换并冷重开。
第二代删除旧 inline CLR、保留同 nominal history，显式升级其一个业务字段及 owner，并升级共享容器内容。
验证仅升级且仍 live 的对象强制 Base，NoChange 后正常产生 Delta，再次冷重开不重复升级。
不为了覆盖迁移而改变容器 nominal 身份；构建新 history 同时核对旧文件 filename/hash/bytes。

由主线程串行运行根 build、相关 tests、根 tests、新包 lane，再用同 feed 回归 BclScalar、Nullable、
CompositeDictionary、Generic/Record 中受公共生成或 history 修改影响的 lane；不并行争用 Windows 输出目录。
实际执行结果集中记录在 §9，不以施工计划代替验收证据。

## 7. 分工、停止条件与后继

建议 G0 由主线程固定公共签名/tag/格式，再分派互斥的字节 codec、SG、Shared/Build history、
图测试与包消费者任务；Runtime/StateStore 的共同绑定入口由主线程集成。
独立 reviewer 检查 offset 比较、字典两种相等性、历史门槛和普通/Family 一致性。
以集成 diff、实际测试、包和文档链接检查验收，不以 subagent 摘要代替。

若发现三值无法用公开 API 无环境依赖地精确往返，或必须改变旧 history 的含义，应带具体反例重新裁决；
不能静默丢 offset、换 UTC 或扩大 DateTime 支持来绕过问题。局部函数名/文件布局可因地制宜。

本片完成后，PROJECT 更新能力与当前焦点，target 提升已采纳的持久语义，路线图收窄剩余日期时间边界。
ValueTuple 的多 child exact/参数来源、跨程序集、SchemaStore 自举继续各自保留重访条件，不借三值片预建扩展点。

## 8. 规划审阅记录

主线程与独立选片评估均推荐三值先行；没有发现足以让跨程序集或 SchemaStore 自举成为前置的新消费者。
另一路平台核验区分了 DateTimeOffset 的完整表示与 DateTime Local 的环境依赖。
多 child 值布局的事实调查用于核对 Tuple 的真实成本，不将“本片不做”表述为技术不可行。
完整草案经两路独立审阅均无阻断；补入正负 offset 独立 golden、clock 上界与旧 history 接受集合的明确验收。
规划阶段仅进行源码/平台资料核验，没有产品代码或测试修改；实施结果见下节。
规划验收：4 份 Markdown 的 234 个本地链接、14 个锚点检查通过，集成 diff 无空白错误；未运行 build/tests。

## 9. 实施合同与验收账本

施工基线 `c7b58a3`，工作区干净。本轮实施 §3–§6 的三值完整纵向范围；DateTime、Tuple、
跨程序集、联合 Store 与性能平台不在范围内。固定 tag 22/23/24、DayNumber/日内 Ticks、
DateTimeOffset clock Ticks + offset 整分钟 Int32 ZigZag；状态比较用 EqualsExact。
新写 history v9，v1–v8 接受集合及已接受文件原样保留；其他格式版本沿 §5。
公开字节入口为 Write/ReadDateOnly、Write/ReadTimeOnly、Write/ReadDateTimeOffset；
字段/元素静态操作和所有权继续复用既有机制，主线程统一集成和串行运行 .NET 验收。

| 要求 | 实现责任 | 验收 | 状态 |
|---|---|---|---|
| G0/G1 三值字节、合法范围与 cursor | Serialization Reader/Writer | [TemporalScalarPayloadTests](../../../tests/DurableGraph.Serialization.Tests/Serialization/TemporalScalarPayloadTests.cs)：37 展开用例；独立 golden、全部 1681 种合法 offset 的边界、非法输入/unmanaged | 已验证 |
| G1 Runtime/目录/标准字典/最低尺寸 | [TemporalScalarStateValues](../../../src/DurableGraph/Runtime/State/TemporalScalarStateValues.cs)、BuiltinStateValues、StateModelSnapshot、DictionaryKeyPolicy | [目录/绑定](../../../tests/DurableGraph.Persistence.Tests/TemporalScalarCatalogTests.cs) 8 例；[静态状态](../../../tests/DurableGraph.Tests/TemporalScalarStateTests.cs) 8 例，包含实际 absent Nullable List 的最低字节预检 | 已验证 |
| G2 普通/Family SG 及 genuine corelib | SG 两条 body、类型识别与模板 history | [TemporalScalarGeneratorTests](../../../tests/DurableGraph.Tests/TemporalScalarGeneratorTests.cs)：16 例，独立编译执行/组合/伪类型/同版变更诊断 | 已验证 |
| G2 v9/旧 history 完整接受集合 | Shared TypePattern、Build history、SG reader | [TemporalScalarHistoryTests](../../../tests/DurableGraph.Tests/TemporalScalarHistoryTests.cs)：14 例；固定 v8、递归门槛、旧接受集合、旧 CLR 删除 | 已验证 |
| G3 同实例图、字典双比较、显式升级 | 既有会话/容器与值规则 | [图](../../../tests/DurableGraph.Tests/TemporalScalarGraphTests.cs)：9 对象冻结隔离、18 次 Commit/原始 Delta/冷重开；[Nullable 升级](../../../tests/DurableGraph.Tests/NullableUpgradeTests.cs)：显式 UTC+08 与空/非空预检 2 例 | 已验证 |
| G3 两代真实包及既有回归 | [TemporalScalarConsumer](../../../experiments/PackageConsumerProbe/TemporalScalarConsumer/README.md) | 新 lane 两代全部 marker 通过；同 feed 的 BclScalar、Nullable、CompositeDictionary、Generic、Record 全通过 | 已验证 |
| 完整集成与文档 | 主线程及独立 reviewer | 根 build 零警告/错误；2290 tests 零失败/跳过；独立审查无阻断，集成 diff/本地链接与锚点通过 | 已验证 |

基线根 build 零警告/错误，原 2204 tests 全通过。新增 86 个展开用例，最终 Runtime/SG 1354、
StateStore 618、Serialization 163、Storage 155 全通过。旧未知 tag 22 改用 25，
Nullable DateTimeOffset 拒绝例改用仍不支持的 BigInteger，保留原负例目的而非删掉测试。

编码与范围按 §3，无合同改向。DateTimeOffset 普通/Family/Runtime 直接调用 EqualsExact；
Shared history 沿用小范围明确能力门槛，独立增加 temporal gate，未展开可选的通用化重构。
只有 history 新写 edition 改为 v9；既有 catalog/Base/Storage/List/Dictionary 格式版本不变。

新包 lane 以隔离八包版本 `0.0.0-temporal-scalar-e2e.20260910073829.40324` 验证，产物位于
`experiments/PackageConsumerProbe/obj/temporal-scalar-20260910073829-40324-bd7cdfb2`。
history 3→5，删除 LegacyPoint 后 retained DTO 可读；owner 与 32 个共享 List 元素显式升级，
仅两对象强制 Base，随后 NoChange→普通 Delta→冷读不重复升级。命令见 §6 与消费者 README，
执行日志位于忽略的 `obj/db058-*`。

同 feed 的 BclScalar、Nullable、CompositeDictionary、Generic（四阶段）、Record 全部 marker 通过。
活动包 runner 的新写 history 断言同步为 v9，已接受 fixture 保留原版本；没有新增旧 State wire 兼容分支。
本片没有改变原设计合同或遗留未完成实现；DateTime/Tuple/跨程序集等既定后继仍按路线图重访。
文档验收覆盖 14 份 Markdown 的 501 个本地链接、47 个锚点；暂存 diff 无空白错误。
